<?php

class Rockrmsinterface extends FreePBX_Helpers implements BMO {

	public function __construct($freepbx) {
		$this->db = $freepbx->Database;
		$this->freepbx = $freepbx;
	}

	public function install() {
		global $amp_conf;

		/* Check if index exists */
		$cdrDb = $this->getDatabase();
		$sql = "SELECT COUNT(*)
FROM information_schema.statistics
WHERE TABLE_SCHEMA = ?
  AND TABLE_NAME = 'cel'
  AND INDEX_NAME = 'rockrms_eventtime_index';";
		$result = $cdrDb->getOne($sql, $amp_conf['CDRDBNAME'] ? $amp_conf['CDRDBNAME'] : 'asteriskcdrdb');

		if ($result == 0) {
			out(_('Creating index on eventtime column, this may take a while.'));
			$cdrDb->query("CREATE INDEX `rockrms_eventtime_index` ON `cel` (`eventtime`);");
		}

		// Common settings.
		$set = array();
		$set['module'] = 'rockrmsinterface';
		$set['category'] = _('RockRMS Interface');
		$set['readonly'] = 0;
		$set['hidden'] = 0;
		$set['level'] = 1;

		// Add config option for outgoing contexts.
		$set['value'] = '';
		$set['defaultval'] =& $set['value'];
		$set['options'] = '';
		$set['emptyok'] = 1;
		$set['name'] = _('Additional Outgoing Contexts');
		$set['description'] = _('A comma separated list of additional contexts to be treated as outgoing calls.');
		$set['sortorder'] = 0;
		$set['type'] = CONF_TYPE_TEXT;
		$this->freepbx->Config->define_conf_setting('ROCK_ADDITIONAL_OUTGOING_CTX', $set);

		// Add config option for incoming contexts.
		$set['value'] = '';
		$set['defaultval'] =& $set['value'];
		$set['options'] = '';
		$set['emptyok'] = 1;
		$set['name'] = _('Additional Incoming Contexts');
		$set['description'] = _('A comma separated list of additional contexts to be treated as incoming calls.');
		$set['sortorder'] = 0;
		$set['type'] = CONF_TYPE_TEXT;
		$this->freepbx->Config->define_conf_setting('ROCK_ADDITIONAL_INCOMING_CTX', $set);

		// Add config option for call timeout,
		$set['value'] = '15';
		$set['defaultval'] =& $set['value'];
		$set['options'] = '';
		$set['emptyok'] = 0;
		$set['name'] = _('Call Timeout');
		$set['description'] = _('The number of seconds a call is tried for before giving up.');
		$set['sortorder'] = 0;
		$set['type'] = CONF_TYPE_TEXT;
		$this->freepbx->Config->define_conf_setting('ROCK_CALL_TIMEOUT', $set);
	}

	public function uninstall() {
		/* Check if index exists */
		$cdrDb = $this->getDatabase();
		$sql = "SELECT COUNT(*)
FROM information_schema.statistics
WHERE TABLE_SCHEMA = ?
  AND TABLE_NAME = 'cel'
  AND INDEX_NAME = 'rockrms_eventtime_index';";
		$result = $cdrDb->getOne($sql, $amp_conf['CDRDBNAME'] ? $amp_conf['CDRDBNAME'] : 'asteriskcdrdb');

		if ($result == 1) {
			out(_('Dropping index on eventtime column, this may take a while.'));
			$cdrDb->query("DROP INDEX `rockrms_eventtime_index` ON `cel`;");
		}

		$this->freepbx->Config->remove_conf_settings('ROCK_ADDITIONAL_OUTGOING_CTX');
		$this->freepbx->Config->remove_conf_settings('ROCK_ADDITIONAL_INCOMING_CTX');
		$this->freepbx->Config->remove_conf_settings('ROCK_CALL_TIMEOUT');
	}

	/**
	 * Unused functions
	 */
	public function backup() {}
	public function restore($backup) {}
	public function doConfigPageInit($page) {}

	/**
	 * Configure settings for incoming AJAX requests.
	 */
	public function ajaxRequest($req, &$setting) {
		$setting['allowremote'] = true;
		$setting['authenticate'] = false;
		return true;
	}

	/**
	 * Handle an AJAX request.
	 */
	public function ajaxHandler() {
		switch ($_REQUEST['command']) {
			case "getCelData":
				$username = isset($_REQUEST['username']) ? $_REQUEST['username'] : '';
				$password = isset($_REQUEST['password']) ? $_REQUEST['password'] : '';
				if (!$this->authenticateUser($username, $password)) {
					throw new Exception("Not Authenticated");
				}

				//
				// Get parameters from the query string.
				//
				$limit = isset($_REQUEST['limit']) ? (int)$_REQUEST['limit'] : 100;
				if ($limit > 1000) {
					$limit = 1000;
				}
				$offset = isset($_REQUEST['page']) ? (int)$_REQUEST['page'] - 1 : 0;
				$offset *= $limit;
				$date = isset($_REQUEST['date']) ? $_REQUEST['date'] : '2000-01-01';

				return $this->getCelData($date, $limit, $offset);

			case "originate":
				$username = isset($_REQUEST['username']) ? $_REQUEST['username'] : '';
				$password = isset($_REQUEST['password']) ? $_REQUEST['password'] : '';
				if (!$this->authenticateUser($username, $password)) {
					throw new Exception("Not Authenticated");
				}

				if (!isset($_REQUEST['from'])) {
					throw new Exception("Must specify the caller number");
				}
				if (!isset($_REQUEST['to'])) {
					throw new Exception("Must specify the called number");
				}
				if (!isset($_REQUEST['callerid'])) {
					throw new Exception("Must specify the caller id");
				}

				return $this->originate($_REQUEST['from'], $_REQUEST['to'], $_REQUEST['callerid']);

			default:
				return false;
		}
	}

	/**
	 * Authenticate the given username and password. Returns true if the user
	 * is authenticated and has sufficient permissions.
	 * @param string $username Username to authenticate
	 * @param string $password Password to be authenticated
	 */
	private function authenticateUser($username, $password) {
		$version = explode(',', get_framework_version());
		if ($version[0] < 13) {
			$password = sha1($password);
		}

		$user = new ampuser($username, 'usermanager');
		if ($user->checkPassword($password)) {
			return $user->checkSection('rockrms_interface');
		}

		$user = new ampuser($username);
		if ($user->checkPassword($password)) {
			return $user->checkSection('rockrms_interface');
		}

		return false;
	}

	/**
	 * Get a connection to the CDR database.
	 */
	private function getDatabase() {
		global $amp_conf;
		$dsn = array(
			'phptype'  => $amp_conf['CDRDBTYPE'] ? $amp_conf['CDRDBTYPE'] : $amp_conf['AMPDBENGINE'],
			'hostspec' => $amp_conf['CDRDBHOST'] ? $amp_conf['CDRDBHOST'] : $amp_conf['AMPDBHOST'],
			'username' => $amp_conf['CDRDBUSER'] ? $amp_conf['CDRDBUSER'] : $amp_conf['AMPDBUSER'],
			'password' => $amp_conf['CDRDBPASS'] ? $amp_conf['CDRDBPASS'] : $amp_conf['AMPDBPASS'],
			'port'     => $amp_conf['CDRDBPORT'] ? $amp_conf['CDRDBPORT'] : '3306',
			'database' => $amp_conf['CDRDBNAME'] ? $amp_conf['CDRDBNAME'] : 'asteriskcdrdb',
		);

		return DB::connect($dsn);
	}

	/**
	 * Originate a call between two parties.
	 * @param string $from     The number that is initiating the call
	 * @param string $to       The number to be called
	 * @param string $callerId The text to use as the callerId when calling $from
	 */
	private function originate($from, $to, $callerId) {
		global $astman, $bootstrap_settings;

		if (!$bootstrap_settings['astman_connected']) {
			throw new Exception('Cannot connect to Asterisk Manager');
		}

		$timeout = ((int)$this->freepbx->Config->get_conf_setting('ROCK_CALL_TIMEOUT')) * 1000;
		$channel = 'Local/' . $from . '@from-internal';
		$toContext = 'from-internal';
		$toPriority = '1';
		$response = $astman->Originate($channel, $to, $toContext, $toPriority, $timeout, $callerId, "", "", "", "");
		return array('status' => $response['Response'], 'message' => $response['Message']);
	}

	/**
	 * Get data from the CEL table.
	 * @param string  $date   The date to begin retrieving records from
	 * @param integer $limit  The maximum number of records to retrieve
	 * @param integer $offset The number of records to skip
	 */
	private function getCelData($date, $limit, $offset) {
		$db = $this->getDatabase();

		//
		// Get the end records that match our criteria.
		//
		$rows = $this->getCelEndRecords($db, $date, $limit, $offset);

		$finalData = array();
		foreach ($rows as $row) {
			/* Get Start */
			$start = $this->getCelStart($db, $row['uniqueid'], $row['id']);
			if ($start == null) {
				continue;
			}

			//
			// Get data from the start record. Sometimes the end record peer
			// gets it's value changed when calls are parked or things like that.
			// So to deal with that we use the start peer record, if we have one.
			//
			$row['starttime'] = $start['eventtime'];
			$row['peer'] = $start['peer'] != '' ? $start['peer'] : $row['peer'];
			$row['duration'] = strtotime($row['endtime']) - strtotime($row['starttime']);

			/* Get Peers */
			$peer1 = $this->getCelPeer($db, $row['endtime'], $row['peer']);
			$peer2 = $this->getCelPeer($db, $row['endtime'], $start['channame']);
			if ($peer1 == null || $peer2 == null) {
				continue;
			}

			//
			// Determine which peer is the source peer.
			//
			if ($peer1['id'] < $peer2['id']) {
				$row['src'] = $peer1['cid_num'];
				$row['src_name'] = $peer1['cid_name'];
				$row['direction'] = $this->getContextDirection($peer1['context']);
				$row['dst'] = $peer2['cid_num'];
				$row['dst_name'] = $peer2['cid_name'];
			}
			else {
				$row['src'] = $peer2['cid_num'];
				$row['src_name'] = $peer2['cid_name'];
				$row['direction'] = $this->getContextDirection($peer2['context']);
				$row['dst'] = $peer1['cid_num'];
				$row['dst_name'] = $peer1['cid_name'];
			}

			//
			// Clean up the result item.
			//
			unset($row['eventtype']);
			unset($row['uniqueid']);
			unset($row['peer']);

			$finalData[] = $row;
		}

		return $finalData;
	}

	/**
	 * Get the end records that match the parameters.
	 * @param object  $db     The database connection to query
	 * @param string  $date   The date to begin retrieving records from
	 * @param integer $limit  The maximum number of records to retrieve
	 * @param integer $offset The number of records to skip
	 */
	private function getCelEndRecords(&$db, $date, $limit, $offset) {
		$sql = "
SELECT
	`bend`.`id`
	, `bend`.`eventtype`
	, `bend`.`uniqueid`
	, `bend`.`eventtime` AS `endtime`
	, `bend`.`peer` AS `peer`
FROM `cel` AS `bend`
WHERE (`bend`.`eventtype` = 'BRIDGE_END' OR `bend`.`eventtype` = 'BRIDGE_EXIT')
  AND `bend`.`eventtime` >= ?
  AND `bend`.`peer` <> ''
	AND `bend`.`channame` NOT LIKE 'Local/%'
	AND `bend`.`peer` NOT LIKE 'Local/%'
ORDER BY `bend`.`eventtime`
";

		$sql = $sql . " LIMIT $offset, $limit";

		return $db->getAll($sql, array($date), DB_FETCHMODE_ASSOC);
	}

	/**
	 * Get the start record associated with this end record.
	 * @param object  $db     The database connection to query
	 * @param string  $uniqueid The uniqueid column value of the end record.
	 * @param integer $id       The id column value from the end record.
	 */
	private function getCelStart(&$db, $uniqueid, $id) {
		$sql = "
SELECT
	`eventtime`
	, `channame`
	, `peer`
FROM `cel`
WHERE `uniqueid` = ? AND `id` < ?
  AND (`eventtype` = 'BRIDGE_START' OR `eventtype` = 'BRIDGE_ENTER')
ORDER BY `id` DESC
LIMIT 1
";

		$result = $db->getAll($sql, array($uniqueid, $id), DB_FETCHMODE_ASSOC);
		if (count($result) == 0) {
			return null;
		}

		return $result[0];
	}

	/**
	 * Get a peer record from the CEL table. This searches for the first CEL
	 * record in the 8 hours prior to the end record that matches the $peername.
	 * @param object $db       The database connection to query
	 * @param string $date     The eventtime column value from the end record
	 * @param string $peername The channel name of the peer to search for
	 */
	private function getCelPeer(&$db, $date, $peername) {
		$sql = "
SELECT
	`id`
	, `cid_name`
	, `cid_num`
	, `context`
FROM `cel`
WHERE `eventtime` BETWEEN DATE_SUB(?, INTERVAL 8 HOUR) AND ?
  AND `channame` = ?
	AND `cid_num` <> ''
ORDER BY `eventtime`,`id` ASC
LIMIT 1
";
		$result = $db->getAll($sql, array($date, $date, $peername), DB_FETCHMODE_ASSOC);
		if (count($result) == 0) {
			return null;
		}

		return $result[0];
	}

	/**
	 * Determines the direction from the context.
	 * @param string $context The context of the source call
	 */
	private function getContextDirection($context) {
		$outgoing = array('from-internal');
		$incoming = array('from-digital', 'from-analog', 'from-trunk',
			'from-pstn');

    $cfg = $this->freepbx->Config->get_conf_setting('ROCK_ADDITIONAL_OUTGOING_CTX');
		if ($cfg != '') {
			$outgoing = array_merge($outgoing, split($cfg, ','));
		}

		$cfg = $this->freepbx->Config->get_conf_setting('ROCK_ADDITIONAL_INCOMING_CTX');
		if ($cfg != '') {
			$incoming = array_merge($incoming, split($cfg, ','));
		}

		if (in_array($context, $outgoing)) {
			return 'outgoing';
		}

		if (in_array($context, $incoming)) {
			return 'incoming';
		}

		return 'unknown';
	}
}
